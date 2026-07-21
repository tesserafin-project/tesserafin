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
/// The count-shaped slot slice 75a leaves <see langword="null"/>: the 75a mapping comparison does
/// not read the raw body, so on a diagnostic that carries no <see cref="StructuralScan"/> this is
/// honestly null (unknown, never a lying 0). Slice 75b's bounded structural scan reports its own
/// truthful total on <see cref="ContractStructuralScan.UnknownMemberTotal"/> instead of mutating
/// this field, so every slice-75a assertion that this stays null keeps holding while the scanned
/// total is still available (on <see cref="StructuralScan"/>) when a scan actually ran.
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
/// <param name="StructuralScan">
/// Issue #75 slice 75b: the result of the bounded, single-pass structural scan of the raw request
/// body, or <see langword="null"/> when no scan ran for this diagnostic - which is the case for
/// every slice-75a-only diagnostic, and whenever the request was not sampled for scanning. Trailing
/// and nullable so every slice-75a call site and OpenAPI consumer keeps working unchanged; a
/// non-null value is the signal that the scan actually executed (a silently-disabled scan cannot
/// present one). Like the rest of this type, its entire closure is enums and integers - see
/// <see cref="ContractStructuralScan"/>.
/// </param>
public sealed record ContractMappingDiagnostic(
    int MappingVersion,
    long? PayloadSizeBytes,
    int? UnknownMemberTotal,
    IReadOnlyList<ContractMappingDelta> Deltas,
    IReadOnlyList<ContractFieldIssue> FieldIssues,
    ContractStructuralScan? StructuralScan = null);
