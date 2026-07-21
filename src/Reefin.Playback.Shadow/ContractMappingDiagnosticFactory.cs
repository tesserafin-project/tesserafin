using System.Collections.Generic;
using Reefin.Playback.Contract.Diagnostics;
using Reefin.Playback.Decision;

namespace Reefin.Playback.Shadow;

/// <summary>
/// Issue #75, Option 1: builds a <see cref="ContractMappingDiagnostic"/> by comparing the
/// <see cref="ClientCapabilities"/> a client DECLARED against the <see cref="ClientCapabilities"/>
/// the server actually planned with, after the request made its real round trip through
/// <c>ReverseDlnaAdapter.ToDeviceProfile</c> (v2 contract -&gt; legacy <c>DeviceProfile</c>, because
/// legacy <c>StreamBuilder</c> is still the source of truth) and back through
/// <c>DlnaPlaybackAdapter.ToCapabilities</c>.
/// </summary>
/// <remarks>
/// <para>
/// This lives here, and not in <c>Reefin.Playback.Contract.Diagnostics</c>, on purpose: comparing
/// capabilities means handling codec/container names, which are text. Keeping the comparison on
/// this side of the wall lets the diagnostics assembly stay literally string-free and ban
/// <c>System.String</c> outright. Nothing read here can reach the returned diagnostic: only counts
/// and presence flags cross back.
/// </para>
/// <para>
/// Only LOSSES are reported. The reverse mapper legitimately SYNTHESIZES entries - a declared
/// direct-play combination reappears as per-codec capability entries, so a count can grow across
/// the round trip without anything having been lost. Reporting a growth as a delta would drown the
/// real signal, so a member is listed only when it lost entries (<c>CountBefore &gt; CountAfter</c>)
/// or lost its presence entirely (<c>PresentBefore &amp;&amp; !PresentAfter</c>).
/// </para>
/// </remarks>
public static class ContractMappingDiagnosticFactory
{
    /// <summary>
    /// The server-side mapping version stamped on every diagnostic this factory produces. Bump it
    /// whenever the set of members compared below changes, so two diagnostics from different server
    /// builds are never read as directly comparable.
    /// </summary>
    public const int MappingVersion = 1;

    private static readonly IReadOnlyList<ContractFieldIssue> _noFieldIssues = [];

    /// <summary>
    /// Builds the diagnostic for one planning call.
    /// </summary>
    /// <param name="declared">
    /// The capabilities the client declared in the request body, or <see langword="null"/> when the
    /// caller is not the v2 contract (the legacy <c>MediaInfoHelper</c> path has no declared domain
    /// capabilities to compare against - it starts from a <c>DeviceProfile</c>). Null yields a null
    /// diagnostic: an absent comparison is reported as absent, never as an empty one.
    /// </param>
    /// <param name="mapped">The capabilities the server actually planned with, after the round trip.</param>
    /// <param name="payloadSizeBytes">
    /// The request's <c>Content-Length</c>, or <see langword="null"/> when the header was absent.
    /// Read from the header, never derived by reading the body.
    /// </param>
    /// <param name="structuralScan">
    /// Issue #75 slice 75b: the bounded structural scan of the raw request body, or
    /// <see langword="null"/> when the request was not scanned (shadow off, or not sampled). Folded
    /// verbatim into the returned diagnostic's <see cref="ContractMappingDiagnostic.StructuralScan"/>;
    /// its non-null presence is what makes a scan that actually ran distinguishable from one that did
    /// not. This factory neither reads nor reshapes it - the closed result already carries only
    /// counts.
    /// </param>
    /// <returns>The diagnostic, or <see langword="null"/> when there is nothing to compare.</returns>
    public static ContractMappingDiagnostic? Create(
        ClientCapabilities? declared,
        ClientCapabilities? mapped,
        long? payloadSizeBytes,
        ContractStructuralScan? structuralScan = null)
    {
        if (declared is null || mapped is null)
        {
            return null;
        }

        var deltas = new List<ContractMappingDelta>();

        // Collections. Count is the signal; presence is derived from it, so a member that was
        // declared empty and stayed empty is never reported as a presence loss.
        AddCollectionDelta(deltas, ContractPath.DecodeDirectPlayProfiles, Count(declared.Decode?.DirectPlayProfiles), Count(mapped.Decode?.DirectPlayProfiles));
        AddCollectionDelta(deltas, ContractPath.DecodeVideoCodecs, Count(declared.Decode?.VideoCodecs), Count(mapped.Decode?.VideoCodecs));
        AddCollectionDelta(deltas, ContractPath.DecodeAudioCodecs, Count(declared.Decode?.AudioCodecs), Count(mapped.Decode?.AudioCodecs));
        AddCollectionDelta(deltas, ContractPath.DecodeSubtitleDelivery, Count(declared.Decode?.SubtitleDelivery), Count(mapped.Decode?.SubtitleDelivery));
        AddCollectionDelta(deltas, ContractPath.OutputProfiles, Count(declared.OutputProfiles), Count(mapped.OutputProfiles));

        // Scalars. A declared boolean that comes back false is the clearest case-(b) evidence this
        // iteration produces: the legacy DeviceProfile has no dedicated slot for these flags (they
        // are re-derived from TranscodingProfiles on the way forward), so a client that declares
        // SupportsHls without also declaring an HLS transcoding target loses the declaration
        // outright. The delta names the path; it never says what the value was beyond present/absent.
        AddScalarDelta(deltas, ContractPath.DecodeSupportsHls, declared.Decode?.SupportsHls ?? false, mapped.Decode?.SupportsHls ?? false);
        AddScalarDelta(deltas, ContractPath.DecodeSupportsDash, declared.Decode?.SupportsDash ?? false, mapped.Decode?.SupportsDash ?? false);

        // The OUTER UnknownMemberTotal stays null, never 0: the 75a mapping comparison still cannot
        // observe unknown members, so its own count-shaped slot remains honestly unknown. The 75b
        // structural scan's truthful total (0 or more) rides on structuralScan.UnknownMemberTotal
        // instead - see ContractMappingDiagnostic. structuralScan is null on every request the scan
        // did not run for, which keeps this diagnostic identical to a 75a-only one for those.
        return new ContractMappingDiagnostic(MappingVersion, payloadSizeBytes, null, deltas, _noFieldIssues, structuralScan);
    }

    private static int Count<T>(IReadOnlyList<T>? values) => values?.Count ?? 0;

    private static void AddCollectionDelta(List<ContractMappingDelta> deltas, ContractPath path, int before, int after)
    {
        if (before <= after)
        {
            return;
        }

        deltas.Add(new ContractMappingDelta(path, before > 0, after > 0, before, after));
    }

    private static void AddScalarDelta(List<ContractMappingDelta> deltas, ContractPath path, bool before, bool after)
    {
        if (!before || after)
        {
            return;
        }

        // Counts stay 0 for a scalar member: the presence flags carry its entire signal, and a
        // fabricated 1/0 would read as a collection that lost an entry.
        deltas.Add(new ContractMappingDelta(path, true, false, 0, 0));
    }
}
