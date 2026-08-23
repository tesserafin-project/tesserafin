# CodeQL `cs/user-controlled-bypass` on `GetDynamicSegment` — diagnosis and repair

Alert 93 on PR #252, rule `cs/user-controlled-bypass` (security-severity 7.5), reported at
`Tesserafin.Api/Controllers/DynamicHlsController.cs:1494`.

## What the analysis actually said

Pinned from the run's own SARIF (analysis `1659115361`, ref `refs/pull/252/merge`, commit
`63f2e92f42f555ebec072405fffbdbb4300ef534`, whose tree is identical to the PR head
`f12e4627f62073ef566c6ae29ee8dbb38248d3f5`):

| component | version |
| --- | --- |
| CodeQL CLI | 2.26.3 |
| `codeql/csharp-queries` | 1.9.1+44a68d3a47fcbcd6a6a76ec7d1c1b3a1a28b201e |
| `codeql/csharp-all` | 7.1.2+44a68d3a47fcbcd6a6a76ec7d1c1b3a1a28b201e |
| `codeql/threat-models` | 1.0.55+44a68d3a47fcbcd6a6a76ec7d1c1b3a1a28b201e |
| `tesserafin/csharp-log-barriers` | 0.0.1 |
| query | `Security Features/CWE-807/ConditionalBypass.ql` |

Four thread flows, nine steps each, two sources and one sink:

* sources — `startTimeTicks` on `GetHlsVideoSegment` (line 1174) and on `GetHlsAudioSegment`
  (line 1358), each a `[FromQuery] long?` action parameter;
* through — the `VideoRequestDto` / `StreamingRequestDto` initialiser, the local
  `streamingRequest`, and the call into `GetDynamicSegment`;
* sink — the guard `if ((streamingRequest.StartTimeTicks ?? 0) > 0)` at line 1494, which stands in
  front of `_jobOwnership.AuthorizeByOutputPath(HttpContext, playlistPath)`.

The alert is PR-specific because both the guard and the ownership call arrived with this branch;
the hosted view is additionally narrowed by the `codeql-action/pr-diff-range` extension, which a
local run does not have.

**The finding is fair even though the guard threw.** `throw` is fail-closed and no caller ever
skipped authorization. What the rule objects to is the SHAPE: a query parameter decides whether an
authorization call is reached at all, so "this route always asks who you are" stops being a
property of the method and becomes a property of one `if` that a later edit could invert.

## Reproduction, locally, with the same CLI and the same pack

`codeql database create --build-mode=manual --command 'dotnet build Tesserafin.sln'`, then the
single query above, against the same CodeQL 2.26.3 bundle:

| tree | `cs/user-controlled-bypass` results |
| --- | --- |
| `master` (`c0f39e07`) | `UserController.cs:291` only — no `DynamicHlsController` alert |
| PR head (`f12e4627`) | `UserController.cs:291` **and** `DynamicHlsController.cs:1494` |
| repaired candidate | `UserController.cs:291` only |

The head reproduction is exact, not merely similar: same sink range `1494:13-55`, same four thread
flows from the same two sources, and the same `partialFingerprints.primaryLocationLineHash`
`b8ccbd9e958ab352:1` the hosted SARIF carries.

`UserController.cs:291` is alert 67, which belongs to `master` and to another piece of work. It is
untouched here.

## What the route did before, measured

Through the real HTTP pipeline, as the job's owner (durable token) unless stated:

| `startTimeTicks` | before | after |
| --- | --- | --- |
| absent | reaches the action | reaches the action |
| `0` | reaches the action | reaches the action |
| positive | `ArgumentException` → `ExceptionMiddleware` → 400, `Error processing request.` | 400, **zero bytes**, refused at the MVC boundary |
| negative | **reached the action** — `> 0` let it through | 400, zero bytes |
| `long.MaxValue` | 400 as above | 400, zero bytes |
| `long.MinValue` | reached the action | 400, zero bytes |
| unparseable (`abc`) | 400 + `ValidationProblemDetails` from model binding | unchanged |
| any value, no credential | 401 | 401 — authorization runs before the boundary |

Two deliberate changes are recorded here rather than left to be discovered:

* **Negative values are now refused.** The old guard tested `> 0`, so a negative start offset flowed
  into `ResolveStreamingState`. A negative offset is a caller-supplied seek on a route that cannot
  honour one, exactly like a positive one. The rule is now "absent or zero, nothing else".
* **The refusal carries no body.** The old refusal was an exception mapped to 400 with
  `Error processing request.`; the boundary answers 400 and nothing else. `BadRequestResult` was
  tried first and is wrong here: `[ApiController]` maps every `IClientErrorActionResult` through
  `ClientErrorResultFilter` into a `ProblemDetails` document, which would answer a media segment
  request with JSON. `ContentResult` with only a status set is not client-error-mapped.

The unparseable row is framework behaviour and is unchanged: model binding fails, and
`[ApiController]`'s model-state filter answers 400 with a validation document ahead of every action
filter. It is a refusal with a body, it is identical on every typed parameter of every route in this
server, and it is never the media.

## The repair

`RejectsStartTimeTicksAttribute`, an `IActionFilter` on `GetHlsVideoSegment` and
`GetHlsAudioSegment`. It reads the bound argument, refuses a non-zero value with a status-only 400,
and does so before the action body runs — so `GetDynamicSegment` contains no branch on the request
at all and `AuthorizeByOutputPath` is the first decision it takes.

Not chosen, and why:

* **`[Range]` or another data annotation.** The check would run inside model binding with no
  `HttpContext` in scope, so "the validation and the ownership decision are about the same request"
  would not be observable. A filter is the boundary where it is.
* **Moving the guard below `ResolveStreamingState`.** That resolves a stream for a value the route
  has already decided it cannot serve, which is the opposite of the property being defended.
* **Changing `StreamingRequestDto`.** The type is shared with routes that legitimately accept a
  start offset.

The filter addresses its parameter by name, the way MVC addresses a bound argument, and **fails
closed** if that name is not on the action: a rename that left the attribute behind would otherwise
make it inert, and inert is indistinguishable from green.

## Evidence

`DynamicHlsStartTimeTicksBoundaryTests` (unit, 21 rows) — the boundary is on exactly the two segment
actions and no others; the declared parameter name resolves to a `long?`; forbidden values are
refused and allowed ones are not; a refused request reaches no collaborator, with the allowed value
as the anti-vacuity control; the authorizer is asked exactly once, on the request's own
`HttpContext` (`Assert.Same`); the fMP4 init map at `segmentId = -1` is covered by the same
boundary; and the ownership matrix — owner, other user, other device, foreign capability, anonymous
— is unchanged, driven through the real action against the real `HlsJobOwnershipAuthorizer` with an
allowed `startTimeTicks` present in the request.

`DynamicHlsStartTimeTicksHttpMatrixTests` (integration, 18 rows) — the same boundary against a
booted server, one real HTTP request at a time. This is the only evidence that the FRAMEWORK
discovers and runs the filter; a filter MVC never invokes is green in every unit test.

Nine hostile controls in `ci/hostile-controls/manifest.json`, all RED with no undeclared collateral
and byte-identical restoration, on a run whose grader was replayed against the ten counter-controls
first (`--self-test`: HELD): restoring the inline guard, removing the attribute, neutering the
refusal, relocating the refusal after the stream is resolved, relocating it after the output path is
named, removing the authorizer call, making the authorizer call conditional on `StartTimeTicks`,
authorizing a caller who is not the owner, and giving the refusal a body while leaving its status
correct.

`cql-restore-the-inline-guard` is the CodeQL control expressed as the source property CodeQL reads.
The CodeQL half of it is the measurement recorded above — restoring the guard on the repaired tree
reproduces the alert, at the guard's own line, with the same four thread flows — and it needs a C#
database, which the roster cannot build. The roster row pins the source shape instead, and says so.

Absence of the alert is not on its own evidence of anything: deleting `AuthorizeByOutputPath`
ALSO makes CodeQL green, and reds the ownership matrix. That is `cql-remove-authorizer-call`.

No alert was dismissed, no `SuppressMessage` or pragma was added, and neither the CodeQL suite nor
its workflow was touched.
