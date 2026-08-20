# Live TV HLS — carrying a validated capability into the segment uris

`#153-LTV-S1`. Stacked on `#153-LTV-S0` (credentialless ffmpeg handoff), which must not be weakened
by anything here.

## The problem this closes

`#153-LTV-S0` made the Live TV transcode work: ffmpeg reads the tuner over `pipe:0` and never
fetches an authorized URL. The browser still could not play, because the playlist ffmpeg writes
points at `Videos/{itemId}/hls/{playlistId}/{segmentId}.{segmentContainer}` — a route that was
hardened to require a playback capability — with **bare relative uris carrying no query at all**.
The client never sees those uris until it reads the playlist, so it cannot credential them itself.

Measured on the rig at the S0 tip, both segment containers:

| Request | Result |
|---|---|
| `master.m3u8`, `live.m3u8` with a capability | 200 |
| segment / `EXT-X-MAP` init map, no query | 401, 0 bytes |

## Why the segment route's demand had to change

The route carried `[RequiresPlaybackCapability(Media, "itemId", null)]`. A `null` media-source
demand agrees — `PlaybackCredentialService.StringsAgree` is exact in both directions — **only** with
a capability that is itself bound to no media source. Every capability a transcoding client mints
for a playback is bound to one, so no such capability could ever reach a Live TV segment. Measured:

| Capability | `live.m3u8` (with `MediaSourceId`) | segment | init map |
|---|---|---|---|
| bound to the media source | 200 | 401 | 401 |
| item-only | 401 | 200 | 200 |

No single capability satisfied both routes in the shape the client emits, and a manifest
transformer alone could not have changed that: it would have propagated the capability correctly
and the segment would still have answered 401.

So `GetHlsVideoSegmentLegacy` now reads `mediaSourceId` from the request, exactly as
`GetHlsPlaylistLegacy` in the same controller already did. This **widens which capability satisfies
the route, not which item it reaches**: the item binding is unchanged and still exact, and a
request that names no media source still refuses a bound capability — the two legacy *audio*
segment routes keep that property under test.

## The transformer

`HlsManifestCredentialPropagator.Propagate(manifest, capability, mediaSourceId, origin)`, applied to
the **response** of `Videos/{itemId}/live.m3u8` and to nothing else.

Applying it to the response rather than to the file makes four clauses true by construction rather
than by assertion: the `.m3u8` on disk stays credential-free, and ffmpeg's argv, environment and
logs never see the value. `-hls_base_url` is untouched.

### Where the capability comes from

From `HttpContext.Items`, written in the **`IsValid` branch only** of
`RequiresPlaybackCapabilityAttribute`, as `ValidatedPlaybackCapability`. Never from
`Request.Query["playbackCapability"]` read a second time: that re-reads an attacker-controlled
string with no proof anything accepted it, and on a route where the capability is optional it would
propagate a value that was never checked. No capability was validated ⇒ no record ⇒ no propagation,
and nothing is ever minted to fill the gap.

The media source travels with it because validation already proved the route's value and the
capability's binding are the same string.

### Uri forms

| Form | Disposition |
|---|---|
| segment lines, relative / root-relative / absolute same-origin | capability appended |
| `#EXT-X-MAP:URI="…"`, same classes | capability appended inside the quotes |
| absolute **external**, including protocol-relative | never enriched |
| `#EXT-X-KEY`, `#EXT-X-SESSION-KEY` | **refused** — a key server must never receive a media capability |
| `#EXT-X-PART`, `#EXT-X-PRELOAD-HINT`, `#EXT-X-RENDITION-REPORT` | **refused** — low-latency forms this muxer configuration does not produce |
| any other tag carrying `URI="…"` | **refused** |

Fail-closed is deliberate. A manifest shape this code does not fully understand must not be served
with a credential in it, and a silently passed-through uri would reach the client uncredentialed and
read as a playback bug rather than an unhandled form.

### Invariants

- Existing queries are carried across as their own bytes, never re-serialized, so every value is
  encoded exactly once.
- A fragment stays last.
- One `playbackCapability` per uri. Already present and identical ⇒ left alone. Present twice, or
  present with a different value ⇒ refused.
- `ApiKey`, `api_key`, `Authorization` and `webSocketTicket` can never be appended: the append path
  rejects those names outright.
- Line endings are preserved per line, so a CRLF manifest stays CRLF and a mixed one stays mixed.
- Comments, blank lines and every non-uri tag are returned unchanged.
- A credential-bearing playlist response is `private, no-store`.
- No mint per fragment. One capability, renewable in place, reused for the whole playlist.
- No non-Live-TV path is touched — `hls1` and the modern `DynamicHlsHelper` master playlist are not
  on this code path at all.

## Scheme detection

`IsExternal` detects a scheme by hand instead of asking `Uri.TryCreate`. On Unix,
`Uri.TryCreate("/videos/x/seg.ts", UriKind.Absolute, …)` **succeeds** with scheme `file`, so a
root-relative segment uri would be classified as foreign-origin and left uncredentialed. That was
measured, not anticipated: `RootRelativeUri_IsEnriched` failed on the first run.
