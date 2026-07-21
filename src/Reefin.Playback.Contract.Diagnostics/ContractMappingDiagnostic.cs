using System.Collections.Generic;

namespace Reefin.Playback.Contract.Diagnostics;

/// <summary>
/// Issue #75, Option 1: what the server can honestly say about ONE playback request's journey
/// through the capability mapping, using a strictly closed, server-owned vocabulary.
/// </summary>
/// <remarks>
/// <para>
/// STRUCTURAL CLOSURE. Every type transitively reachable from this one is an enum declared in this
/// assembly, a <see cref="bool"/>, an <see cref="int"/>/<see cref="long"/>, a nullable of one of
/// those, or a read-only list of one of those. There is no <c>string</c>, <c>Guid</c>,
/// <c>object</c>, dictionary, <c>JsonElement</c>, <c>byte[]</c>, or any other extensible value
/// anywhere in the closure - so no value a client sent can be carried here even by a future
/// mistake. Two independent mechanisms hold that: <c>ContractClosureTests</c> walks the closure by
/// reflection, and this assembly's own <c>BannedSymbols.txt</c> fails the BUILD if such a member is
/// ever declared.
/// </para>
/// <para>
/// WHAT THIS ACTUALLY DELIVERS, without overselling it. Option 1 has no structural scan of the raw
/// request body (reading it would require request buffering, which is out of scope, and would
/// re-open exactly the raw-payload question issue #35 was closed over). The consequences are:
/// unknown members are UNOBSERVABLE, not merely unnamed (see <see cref="UnknownMemberTotal"/>);
/// a wrong type on a text member is undetectable once the model binder has coerced it; an
/// out-of-range value generally leads to the request being REJECTED before the shadow publication
/// point, so no diagnostic is created for it at all. The signal genuinely delivered is
/// <see cref="Deltas"/> - the mapping delta, i.e. the distinction between issue #75's case (b)
/// (information lost mapping into <c>ClientCapabilities</c>) and case (c) (a decision-engine
/// regression).
/// </para>
/// <para>
/// LIFECYCLE. Produced only behind the EXISTING shadow gate (<c>PlaybackShadowOptions</c>, off by
/// default, sampled by the existing sample rate), retained in the EXISTING
/// <c>IShadowDiagnosticsStore</c> under the EXISTING per-session TTL, and served by the EXISTING
/// admin diagnostics route. No new store, endpoint, TTL, or switch exists for it. A request
/// rejected before the shadow publication point never produces one.
/// </para>
/// </remarks>
/// <param name="MappingVersion">
/// The server-side mapping version this diagnostic was produced under - a compile-time server
/// constant, never anything the client influences. Lets two diagnostics from different server
/// builds be told apart.
/// </param>
/// <param name="PayloadSizeBytes">
/// The total request payload size in bytes, read from the <c>Content-Length</c> header alone, or
/// <see langword="null"/> when the header is absent (a chunked or streamed request). Deliberately
/// <see cref="System.Nullable{T}"/>: request buffering is NOT enabled to measure the body, so
/// "unknown" is reported as unknown rather than as a lying 0. A count of bytes, never a byte of
/// content.
/// </param>
/// <param name="UnknownMemberTotal">
/// ALWAYS <see langword="null"/> in this iteration, and that is the honest answer rather than a
/// missing feature. Counting members the client sent that the contract does not declare requires
/// reading the raw body structurally; Option 1 does not do that, so the server does not KNOW the
/// number. Reporting 0 would be a lying zero - indistinguishable from "the client sent no unknown
/// members", which the server cannot establish. Issue #75 permits a count and forbids names; this
/// field is the count-shaped slot, left null until a source exists that can fill it truthfully.
/// </param>
/// <param name="Deltas">
/// The before/after comparison for the known contract members that LOST something across the
/// mapping. Only genuine losses are listed - a member that survived intact, or that grew (the
/// reverse mapper legitimately synthesizes per-codec entries from direct-play combinations),
/// produces no entry. Empty means the mapping preserved everything it is asked about, which points
/// at case (c).
/// </param>
/// <param name="FieldIssues">
/// Closed-vocabulary issues for known members. Empty in this iteration: see
/// <see cref="ContractIssueCode"/> for why each code either has no semantically correct source yet
/// or is structurally unobservable at this point in the pipeline. Present so the shape does not
/// have to change when one of them becomes truthfully emittable.
/// </param>
public sealed record ContractMappingDiagnostic(
    int MappingVersion,
    long? PayloadSizeBytes,
    int? UnknownMemberTotal,
    IReadOnlyList<ContractMappingDelta> Deltas,
    IReadOnlyList<ContractFieldIssue> FieldIssues);
