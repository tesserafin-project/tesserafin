# Issue #43 — the `PlaybackSessionId` / `PlaybackAttemptId` cycle

Design for the full client-owned session cycle over the v2 `Playback/Sessions` protocol:
`PlaybackInfo → POST → GET .../Stream → PUT → retry → DELETE`, plus what happens when the
client never gets to say `DELETE`.

Companion to `docs/pr92-design-playback-api-and-diagnostics.md` (the protocol) and
`docs/design-playback-v2-lifecycle.md` (the canary). Nothing here activates v2: every client
behaviour described below fires **only** for a session v2 actually established, behind the
existing `enableV2PlaybackPath` flag, which stays **off by default**.

---

## 1. The two ids, and who owns each

| Id | Minted by | Scope | Sent on | Server rule |
|---|---|---|---|---|
| `PlaySessionId` | **client** (`crypto.randomUUID()`) | one playback attempt's *stream* | `POST` body | key into `_byPlaySessionId`; reused id → replace-in-place |
| `PlaybackSessionId` | **server** (`PlaybackSessionId.NewId()`) | one server-side session resource | returned by `POST`, used in `PUT`/`DELETE`/`GET .../Stream` paths | primary key of `_sessions` |
| `PlaybackAttemptId` | **client** (`playbackAttemptId.ts`) | one *user action*, spans retries | `PlaybackInfo`, `POST`, `PUT` | diagnostics only; `?? existing` on re-plan |

The distinction that carries the whole design: **`PlaySessionId` is reused across a
replacement, `PlaybackSessionId` is not.** A `POST` carrying an already-known `PlaySessionId`
returns the *same* `PlaybackSessionId` (`StoreOrReplace`'s replace branch). A genuinely new
attempt mints a new `PlaySessionId` and therefore gets a new `PlaybackSessionId`.

**Teardown keys on `PlaybackSessionId`, never on `PlaySessionId`.** This is not a preference;
it is the entire mechanism by which requirement "an old teardown must not delete a new
session" is satisfied — see §5.

### 1.1 `PlaybackAttemptId` authorizes nothing

Load-bearing, and worth stating because it is the kind of field that drifts into an
authorization key. The server's ownership check is
`PlaybackSessionsController.EnsureCallerOwnsSessionOrIsAdmin`, which reads
`session.Request?.Options.UserId` and the caller's `Administrator` role — `PlaybackAttemptId`
appears nowhere in it, and no client behaviour branches on the value either. It is a
correlation string: absent is as valid as present, and blank is rejected (`400`) only so a
blank never masquerades as a value.

---

## 2. Client state machine

One instance per player, living on `playerData` (survives `changeStream()`, which replaces
`streamInfo` wholesale).

```
                  POST Playback/Sessions ok
   [idle] ───────────────────────────────────────▶ [established(sessionId, gen)]
      ▲                                                │  │  │
      │                                                │  │  └── PUT (compatible re-plan)
      │                                                │  │      ─▶ [established(sessionId, gen)]
      │  DELETE ok / 404 / gave up                     │  │
      └────────────── [tearing-down] ◀─────────────────┘  │
                                    stop / error / replace │
                                                           │
                              new attempt (new PlaySessionId)
                                                           ▼
                                          [established(sessionId', gen+1)]
```

`gen` is a monotonically increasing client-side counter. Every transition stamps the
generation that produced it; a teardown carries the generation it was scheduled for and is
dropped if the current generation has moved past it (§5).

### 2.1 Transitions

- **`idle → established`** — `POST Playback/Sessions` returns an `Id`. Client records
  `{ sessionId, playSessionId, attemptId, gen }`.
- **`established → established` (PUT)** — a re-plan *within the same attempt* whose semantics
  the server actually supports: same item, same media source, same `PlaySessionId`, changed
  capabilities/constraints. Carries the **same** `PlaybackAttemptId`. See §3.
- **`established → tearing-down`** — playback stopped, errored fatally, or is being replaced.
- **`established → established'` (replacement)** — the next item / a new user Play. Mints a new
  attempt id *and* a new `PlaySessionId`, so the server issues a new `PlaybackSessionId`. The
  outgoing session's teardown is scheduled **before** the new `POST` is issued, but is
  generation-stamped, so its lateness cannot harm the new session.

### 2.2 What is deliberately *not* a PUT

`changeStream()`'s transcoding retry re-enters `getPlaybackInfo()` and rebuilds the stream
from scratch. It is the same *attempt* (same `PlaybackAttemptId`) but a different *stream*.
Modelling it as `PUT` would be wrong: the client re-derives a whole new `streamInfo`, and the
v2 path re-`POST`s with a fresh `PlaySessionId`. So a retry is a **replacement**, not a
`PUT` — new `PlaybackSessionId`, same `PlaybackAttemptId`. `PUT` is reserved for the case
where the client keeps the same stream identity and only re-plans it.

---

## 3. `PUT` — when it is genuinely compatible

`ReplacePlaybackSession` re-plans an **existing** session id. It is safe exactly when the
client is not also changing the stream's identity, because the server keeps
`_byPlaySessionId` pointing at the same session either way. Concretely the client issues a
`PUT` only when **all** hold:

1. the session is `established` and the client still holds its `PlaybackSessionId`;
2. `ItemId` and `MediaSourceId` are unchanged;
3. the `PlaySessionId` is unchanged;
4. only `Capabilities`/`Constraints` differ.

If any of those fail it is a replacement, not a `PUT`. Note `PUT` returns `422` when the new
options have no viable plan (PR #38 deliberately distinguishes this from `404` "unknown id"),
and the client treats `422` as "keep the current session, fall back to legacy" — never as a
reason to tear down a session that is still playing.

---

## 4. Idempotence

| Operation | Repeat behaviour | Where enforced |
|---|---|---|
| `POST` same `PlaySessionId` | replaces in place, **same** `PlaybackSessionId` | `StoreOrReplace` replace branch |
| `PUT` same body | recomputes the same plan, same id | `Patch` |
| `DELETE` twice | second gets `404` | `Delete` → `RemoveNoLock` returns false |
| client teardown twice | second is a no-op, never a second request | client state machine |

The client treats **`404` on `DELETE` as success**. The goal is "the session is not there",
and a `404` means precisely that — whether this client removed it or the server's own
`PlaybackStopped`/`TranscodingJobEnded` handler got there first (both call
`DeleteByPlaySessionId`, which is very often the *real* reason a session disappears before the
client's own `DELETE` lands). Treating `404` as failure would produce retry storms against
sessions the server already correctly reaped.

`403` is **not** retried: it is a permanent statement about this caller.

---

## 5. Protection against a stale teardown

The hazard: attempt A stops, its `DELETE` is slow or queued; attempt B starts and establishes
a new session; A's `DELETE` lands afterwards and kills B.

Two independent mechanisms, either of which alone is sufficient:

1. **Distinct ids (server-side, structural).** A's teardown carries A's `PlaybackSessionId`.
   B has a different one — `StoreOrReplace` mints a fresh `NewId()` whenever the
   `PlaySessionId` is new, and a new attempt always mints a new `PlaySessionId`. A `DELETE`
   for A's id therefore *cannot* address B's session; worst case it `404`s. This is why
   teardown must key on the server id and not on `PlaySessionId`.
2. **Generation guard (client-side, defensive).** Every teardown is stamped with the
   generation it was scheduled for and is dropped before issuing if the tracker has moved on.
   Redundant with (1) by construction, kept because it also suppresses the *useless request*,
   not just its effect, and it protects the client's own bookkeeping from being rolled back by
   a late completion.

The interesting sub-case: a **`POST` that reuses the same `PlaySessionId`** returns the same
`PlaybackSessionId`, so mechanism (1) does not separate them. That is correct, not a hole —
it is genuinely the same session, and the client's tracker replaces its record rather than
creating a second one. The generation guard is what prevents the *previous* teardown from
deleting it, which is exactly the case (1) cannot cover and (2) can.

---

## 6. Teardown guarantees — measured, not assumed

**There is no browser guarantee of a request at teardown.** This is the honest position and
the design does not pretend otherwise. Measurements and method are recorded in
`docs/issue43-browser-teardown-measurements.md` (companion, web repo runs the harness).

Key facts the design is built on:

- **`navigator.sendBeacon` cannot be used at all here**: it issues `POST` only, and this
  protocol's teardown is `DELETE`. It is not an option, not merely a weaker one.
- **`fetch(url, { method: 'DELETE', keepalive: true })`** is the only mechanism that can
  outlive the document. It is *best effort*, subject to a 64 KiB inflight-body cap
  (irrelevant here — the body is empty) and to the browser deciding to honour it.
- **`beforeunload`/`unload` are the least reliable** and are actively skipped when a page is
  discarded from the back/forward cache or the tab is killed.
- **`pagehide` and `visibilitychange → hidden` are the best available signals**, and on mobile
  `visibilitychange` is frequently the *only* one delivered.

Consequences, taken seriously rather than papered over:

- Teardown is fired from `pagehide` **and** `visibilitychange → hidden`, deduplicated by the
  same idempotence machinery as everything else, and issued with `keepalive: true`.
- Because none of that is guaranteed, **the authoritative cleanup remains server-side**:
  `PlaybackSessionManager` already reaps on `PlaybackStopped` and `TranscodingJobEnded`, and
  `SweepExpired` backstops both with a 6 h TTL. The client `DELETE` is an *optimisation* that
  frees the session promptly; correctness does not depend on it.
- A `visibilitychange → hidden` that is **not** a teardown (user switched tabs mid-playback)
  must not kill a live session. So the hidden-state teardown fires only when the tracker is
  already in `tearing-down`/`idle`-pending state — i.e. it flushes a teardown that is already
  owed, and never initiates one for a session still playing.

---

## 7. Reconciliation

Offline and error cases, in order of how the client handles them:

- **Offline / network error on `DELETE`** — the session is left to the server's own reaping.
  The client does **not** queue the teardown for a later online event: a `PlaybackSessionId`
  the server may already have reaped is not worth persisting, and replaying it after an
  arbitrary delay is exactly the stale-teardown hazard §5 exists to prevent. The tracker
  forgets the session; the 6 h TTL is the backstop.
- **`404`** — treated as success (§4).
- **`403`** — logged, not retried. Since the `StoreOrReplace` fix this should no longer occur
  for a session's own owner; if it does, it is a genuine bug and a silent retry would hide it.
- **`5xx`** — one best-effort attempt, no retry loop. Same reasoning as offline.
- **Server-side reap while the client still thinks it holds a session** — detected lazily: the
  next `PUT`/`GET .../Stream` on that id `404`s, which the client already handles by falling
  back to legacy.

**No client-side teardown failure is ever surfaced to the user.** Playback has already ended
by the time any of this runs; an error toast about a cleanup call would be noise.

---

## 8. The `StoreOrReplace` erasure (fixed here)

`StoreOrReplace` assigned `Request = request` unconditionally, while the comment directly
above it claimed the "null does not erase" rule it applies to `PlaybackAttemptId`.
`Track()` — the HLS segment path, `DynamicHlsController` → `PlaybackSessionManager.Track` —
**always** passes `request: null`. So any segment fetch landing on a session a previous
`Create` had established for the same `PlaySessionId` silently nulled the stored request.

That null is load-bearing: `EnsureCallerOwnsSessionOrIsAdmin` reads ownership off
`session.Request?.Options.UserId` and forbids when it is null, and `GetPlaybackSessionStream`
`422`s on the same null. The ordinary client sequence — `POST`, then fetch a segment —
therefore **locked the session's own owner out of it**: `PUT` and `DELETE` answered `403`,
`GET .../Stream` answered `422`, while an administrator was unaffected. That asymmetry is
what made the endpoint read as "admin only".

Issue #43's client teardown is impossible while that holds, `DELETE` being precisely the verb
the owner is refused. Hence the fix lands first, with a test verified RED beforehand.

`Kind` and `Plan` deliberately keep overwriting unconditionally: `Track`'s purpose is to
record the plan actually being executed, and `GET .../Stream` reads `session.Plan.StreamInfo`.
Only `Request` has a caller that legitimately has none to contribute.

---

## 9. Correlation chain

What a diagnostics reader can join, after this work:

```
PlaybackInfo   ── PlaybackAttemptId ──┐
POST /Sessions ── PlaybackAttemptId ──┤ same attempt
   └─ returns PlaybackSessionId  ─────┤
GET  /Sessions/{id}/Stream ───────────┤ same session
PUT  /Sessions/{id} ── PlaybackAttemptId ──┤ same attempt AND same session
retry (changeStream) ── PlaybackAttemptId ─┘ same attempt, NEW session
DELETE /Sessions/{id} ────────────────  same session
```

Each HTTP request additionally carries the issue #42 request id, which differs on every one
of the above — including between a retry and the attempt it retries. That is the point of
having both: the request id separates the calls, the attempt id joins them.
</content>
</invoke>
