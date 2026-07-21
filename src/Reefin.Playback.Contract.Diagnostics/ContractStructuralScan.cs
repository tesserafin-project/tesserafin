using System.Collections.Generic;

namespace Reefin.Playback.Contract.Diagnostics;

/// <summary>
/// Issue #75 slice 75b: the result of the BOUNDED, SINGLE-PASS structural scan of ONE raw playback
/// request body - what the server can honestly count about members the client sent, without ever
/// materializing a key, a value, an excerpt, a truncated form, or a hash of any of them.
/// </summary>
/// <remarks>
/// <para>
/// This is the piece slice 75a (<see cref="ContractMappingDiagnostic"/>) deliberately left out:
/// 75a compared the DECLARED-vs-MAPPED capabilities without reading the raw body, so it could never
/// observe an unknown member and reported <see cref="ContractMappingDiagnostic.UnknownMemberTotal"/>
/// as an honest null. 75b adds a strictly bounded pass over the raw body -
/// <c>Reefin.Playback.Contract.Scan.PlaybackContractScanner</c> using
/// <see cref="System.Text.Json.Utf8JsonReader"/> - that fills the count-shaped slots this type
/// exposes. The scan runs ONLY behind the existing shadow gate and ONLY when the request is
/// sampled: a body that is not scanned produces no instance of this type at all (the enclosing
/// <see cref="ContractMappingDiagnostic.StructuralScan"/> stays null), which is exactly what makes a
/// silently-disabled scan observable rather than green.
/// </para>
/// <para>
/// STRUCTURAL CLOSURE, same wall as the rest of this assembly: every member here is an
/// <see cref="int"/>/<see cref="long"/>, a <see cref="bool"/>, a nullable of one of those, or a
/// read-only list of a closed struct (<see cref="ContractUnknownMemberCount"/>,
/// <see cref="ContractFieldIssue"/>) whose own members are server-owned enums and integers. No
/// <c>string</c>/<c>char</c>/<c>Guid</c>/<c>object</c>/dictionary/<c>JsonElement</c>/<c>byte[]</c>
/// can be reached from here, so nothing the client sent can ride in even by a future mistake -
/// <c>ContractClosureTests</c> and this assembly's <c>BannedSymbols.txt</c> both hold that.
/// </para>
/// </remarks>
/// <param name="UnknownMemberTotal">
/// The total number of members the client sent, anywhere the scan looked, that the contract does
/// not declare. A count, never a name. 0 is meaningful here (unlike on
/// <see cref="ContractMappingDiagnostic.UnknownMemberTotal"/>): the scan actually ran, so "the
/// client sent no unknown members" is a fact the server can now establish rather than a lying zero.
/// </param>
/// <param name="UnknownMembers">
/// The per-container breakdown of <see cref="UnknownMemberTotal"/>: one entry per known container
/// that had at least one unknown member directly inside it. Empty when
/// <see cref="UnknownMemberTotal"/> is 0. The entries sum to <see cref="UnknownMemberTotal"/>.
/// </param>
/// <param name="WrongTypes">
/// Closed-vocabulary <see cref="ContractIssueCode.WrongType"/> issues for KNOWN members only: a
/// member the contract declares as numeric that arrived as a JSON string. The scan judges by JSON
/// token kind alone and never reads the string's content. Which of these actually SURVIVES to a
/// retained diagnostic is bounded by the binder: only a numeric-looking string (e.g. <c>"64000"</c>)
/// binds under <c>NumberHandling.AllowReadingFromString</c> and lets the request reach the shadow
/// publication point; a non-numeric string 400s at the binder, so its (leak-free) WrongType is
/// observed by the scan but never retained. Either way the operator learns only "a known numeric
/// member arrived string-typed under this path", never which value.
/// </param>
/// <param name="ScannedBodyByteCount">
/// The number of bytes the scan actually read from the request body. This is the honest measured
/// size when the <c>Content-Length</c> header was absent (a chunked/streamed request), where
/// <see cref="ContractMappingDiagnostic.PayloadSizeBytes"/> is null. A count of bytes, never a byte
/// of content. Null only if the body was not read (see <see cref="BodyLimitExceeded"/>).
/// </param>
/// <param name="BodyLimitExceeded">
/// True when the request body exceeded the scan's explicit size limit, so the scan stopped without
/// producing counts. The bound is on how much the scan reads; model binding is unaffected and still
/// sees the whole body. Lets "the body was too large to scan" be told apart from "the body was
/// scanned and had no unknown members".
/// </param>
public sealed record ContractStructuralScan(
    int UnknownMemberTotal,
    IReadOnlyList<ContractUnknownMemberCount> UnknownMembers,
    IReadOnlyList<ContractFieldIssue> WrongTypes,
    long? ScannedBodyByteCount,
    bool BodyLimitExceeded);
