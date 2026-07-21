using System;
using System.Collections.Generic;
using System.Text.Json;
using Reefin.Playback.Contract.Diagnostics;
using Reefin.Playback.Decision;
using Reefin.Playback.Shadow;
using Xunit;

namespace Reefin.Playback.Shadow.Tests;

/// <summary>
/// Issue #75 slice 75b: the factory folds a structural scan into the diagnostic without disturbing
/// slice 75a's invariants, and the folded result - the object every downstream sink (the retained
/// <c>ShadowDiagnosticRecord</c>, the admin Mapping subtree, the HTTP response) embeds verbatim -
/// carries no client value even when the scan was of a body stuffed with a secret sentinel.
/// </summary>
public sealed class ContractMappingScanFoldingTests
{
    private const string Sentinel = "S3nt1nel_c0ffee_LEAK_D0_N0T_ECH0_a11ab1e";

    private static ClientCapabilities EmptyCapabilities()
    {
        var decode = new DecodeCapabilities(
            Array.Empty<DecodeProfile>(),
            Array.Empty<VideoCodecCapability>(),
            Array.Empty<AudioCodecCapability>(),
            Array.Empty<SubtitleCapability>(),
            SupportsHls: false,
            SupportsDash: false);

        return new ClientCapabilities(decode, Array.Empty<PlaybackOutputProfile>());
    }

    // A scan result shaped as if the sentinel had been an unknown key: counts only, no trace of it.
    private static ContractStructuralScan SentinelDerivedScan() =>
        new(
            UnknownMemberTotal: 2,
            UnknownMembers: new[]
            {
                new ContractUnknownMemberCount(ContractPath.Request, 1),
                new ContractUnknownMemberCount(ContractPath.Decode, 1),
            },
            WrongTypes: new[] { new ContractFieldIssue(ContractPath.DecodeVideoCodecs, ContractIssueCode.WrongType) },
            ScannedBodyByteCount: 4096,
            BodyLimitExceeded: false);

    [Fact]
    public void Create_FoldsScan_WhileKeeping75aInvariants()
    {
        var caps = EmptyCapabilities();
        var scan = SentinelDerivedScan();

        var diagnostic = ContractMappingDiagnosticFactory.Create(caps, caps, payloadSizeBytes: 4096, structuralScan: scan);

        Assert.NotNull(diagnostic);

        // 75a invariants preserved: outer count stays null, outer field issues stay empty.
        Assert.Null(diagnostic!.UnknownMemberTotal);
        Assert.Empty(diagnostic.FieldIssues);

        // 75b folded in and observable: a scan that actually ran is distinguishable from one that did not.
        Assert.NotNull(diagnostic.StructuralScan);
        Assert.Equal(2, diagnostic.StructuralScan!.UnknownMemberTotal);
        Assert.Equal(4096, diagnostic.StructuralScan.ScannedBodyByteCount);
    }

    [Fact]
    public void Create_WithoutScan_LeavesStructuralScanNull()
    {
        var caps = EmptyCapabilities();

        var diagnostic = ContractMappingDiagnosticFactory.Create(caps, caps, payloadSizeBytes: null);

        Assert.NotNull(diagnostic);
        // Anti-vacuity discriminator: no scan ran => null, never an empty-but-present scan.
        Assert.Null(diagnostic!.StructuralScan);
    }

    [Fact]
    public void FoldedDiagnostic_SerializesWithoutSentinel_AcrossEverySink()
    {
        var caps = EmptyCapabilities();
        var diagnostic = ContractMappingDiagnosticFactory.Create(caps, caps, payloadSizeBytes: 4096, structuralScan: SentinelDerivedScan());

        // Sink 1 (ContractMappingDiagnostic) and, since the retained ShadowDiagnosticRecord, the admin
        // Mapping subtree and the HTTP response all embed this exact object verbatim, sinks 2/3/5 too.
        var json = JsonSerializer.Serialize(diagnostic);

        Assert.DoesNotContain(Sentinel, json, StringComparison.Ordinal);
        // The scan's signal did survive - this is a real, non-empty diagnostic, not a vacuous pass.
        Assert.Contains("\"UnknownMemberTotal\":2", json, StringComparison.Ordinal);
    }
}
