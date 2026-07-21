namespace Tesserafin.Playback.Contract.Diagnostics;

/// <summary>
/// Issue #75 slice 75b: HOW MANY members the client sent that the contract does not declare, under
/// one known container named by a server-owned <see cref="ContractPath"/> - never WHICH members,
/// and never a single character of any of their names.
/// </summary>
/// <remarks>
/// This is the count-shaped slot issue #75 permits, filled truthfully by the bounded structural
/// scan (<c>Tesserafin.Playback.Contract.Scan.PlaybackContractScanner</c>). An unknown member has, by
/// definition, no <see cref="ContractPath"/> of its own - it is not in the server's vocabulary - so
/// it is attributed to the KNOWN container it appeared directly inside (the request root,
/// <c>Capabilities</c>, <c>Capabilities.Decode</c>, or a known codec/profile entry). The scan
/// reaches this attribution using only <see cref="System.Text.Json.Utf8JsonReader.ValueTextEquals(System.ReadOnlySpan{byte})"/>
/// against the server's own member names; the unknown key itself is counted and skipped, never
/// materialized.
/// </remarks>
/// <param name="Path">The known container the unknown members appeared inside.</param>
/// <param name="Count">How many unknown members appeared directly under <paramref name="Path"/>. Always &gt; 0 - a container with no unknown members produces no entry.</param>
public readonly record struct ContractUnknownMemberCount(
    ContractPath Path,
    int Count);
