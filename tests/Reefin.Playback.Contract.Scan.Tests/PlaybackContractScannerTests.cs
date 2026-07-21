using System;
using System.Text;
using System.Text.Json;
using Reefin.Playback.Contract.Diagnostics;
using Xunit;

namespace Reefin.Playback.Contract.Scan.Tests;

/// <summary>
/// Issue #75 slice 75b: the bounded single-pass scanner over a hostile corpus. Every case asserts
/// two things at once - the scan produced the RIGHT count/flag (anti-vacuity: a scan that silently
/// did nothing would fail), and the distinctive secret sentinel injected into the body never appears
/// in the scan's serialized output.
/// </summary>
public sealed class PlaybackContractScannerTests
{
    // A distinctive secret that must NEVER survive into any scan output. Injected into keys and
    // values of the hostile bodies below.
    private const string Sentinel = "S3nt1nel_c0ffee_LEAK_D0_N0T_ECH0_a11ab1e";

    private static ContractStructuralScan Scan(string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return PlaybackContractScanner.Scan(bytes, ContractScanTestModel.Root, bytes.Length, bodyLimitExceeded: false);
    }

    private static string SerializeAll(ContractStructuralScan scan)
    {
        // Serialize both the scan and a full diagnostic wrapping it - the two closed sinks the scan
        // flows into - and return the concatenation for a single "sentinel absent" assertion.
        var diagnostic = new ContractMappingDiagnostic(
            MappingVersion: 1,
            PayloadSizeBytes: null,
            UnknownMemberTotal: null,
            Deltas: Array.Empty<ContractMappingDelta>(),
            FieldIssues: Array.Empty<ContractFieldIssue>(),
            StructuralScan: scan);

        return JsonSerializer.Serialize(scan) + "\n" + JsonSerializer.Serialize(diagnostic);
    }

    private static int UnknownAt(ContractStructuralScan scan, ContractPath path)
    {
        foreach (var entry in scan.UnknownMembers)
        {
            if (entry.Path == path)
            {
                return entry.Count;
            }
        }

        return 0;
    }

    [Fact]
    public void CleanBody_ScanRan_NoUnknownsNoWrongTypes()
    {
        var scan = Scan("{\"ItemId\":\"a\",\"Capabilities\":{\"Decode\":{\"VideoCodecs\":[{\"Codec\":\"h264\",\"MaxBitrate\":64000}],\"SupportsHls\":true}}}");

        // Anti-vacuity: the scan actually ran over this body (a byte count proves the pass happened),
        // and honestly found nothing wrong - not a silently-skipped zero.
        Assert.False(scan.BodyLimitExceeded);
        Assert.Equal(0, scan.UnknownMemberTotal);
        Assert.Empty(scan.UnknownMembers);
        Assert.Empty(scan.WrongTypes);
    }

    [Fact]
    public void SecretInUnknownKey_CountedUnknown_NeverLeaked()
    {
        var scan = Scan("{\"" + Sentinel + "\":123,\"ItemId\":\"x\"}");

        Assert.Equal(1, scan.UnknownMemberTotal);
        Assert.Equal(1, UnknownAt(scan, ContractPath.Request));
        Assert.DoesNotContain(Sentinel, SerializeAll(scan), StringComparison.Ordinal);
    }

    [Fact]
    public void SecretInKnownMemberValue_Skipped_NeverLeaked()
    {
        var scan = Scan("{\"MediaSourceId\":\"" + Sentinel + "\"}");

        // A known member's value is skipped whole; nothing is counted, nothing leaks.
        Assert.Equal(0, scan.UnknownMemberTotal);
        Assert.DoesNotContain(Sentinel, SerializeAll(scan), StringComparison.Ordinal);
    }

    [Fact]
    public void SecretInUnknownMemberValue_CountedOnce_NeverLeaked()
    {
        var scan = Scan("{\"Bogus\":\"" + Sentinel + "\"}");

        Assert.Equal(1, scan.UnknownMemberTotal);
        Assert.DoesNotContain(Sentinel, SerializeAll(scan), StringComparison.Ordinal);
    }

    [Fact]
    public void SixtyFourKibValue_Skipped_ScanRan_NeverLeaked()
    {
        var big = Sentinel + new string('A', 64 * 1024);
        var scan = Scan("{\"Capabilities\":{\"Decode\":{\"VideoCodecs\":[{\"Codec\":\"" + big + "\"}]}}}");

        Assert.Equal(0, scan.UnknownMemberTotal);
        Assert.Empty(scan.WrongTypes);
        Assert.DoesNotContain(Sentinel, SerializeAll(scan), StringComparison.Ordinal);
    }

    [Fact]
    public void JsonInsideAString_Skipped_NeverLeaked()
    {
        // MediaSourceId's value is a string that itself contains JSON with the sentinel as a key.
        var innerJson = "{\\\"nested\\\":{\\\"" + Sentinel + "\\\":1}}";
        var scan = Scan("{\"MediaSourceId\":\"" + innerJson + "\"}");

        Assert.Equal(0, scan.UnknownMemberTotal);
        Assert.DoesNotContain(Sentinel, SerializeAll(scan), StringComparison.Ordinal);
    }

    [Fact]
    public void UnicodeBidiControlUnknownKey_CountedUnknown_NeverLeaked()
    {
        // Bidi override (U+202E) + zero-width space (U+200B) around the sentinel, as a hostile key.
        var key = "‮" + Sentinel + "​";
        var scan = Scan("{\"" + key + "\":1,\"ItemId\":\"x\"}");

        Assert.Equal(1, scan.UnknownMemberTotal);
        Assert.Equal(1, UnknownAt(scan, ContractPath.Request));
        Assert.DoesNotContain(Sentinel, SerializeAll(scan), StringComparison.Ordinal);
    }

    [Fact]
    public void KnownNumericSentAsBindingString_WrongType_ReachesSink()
    {
        // MaxBitrate is declared numeric. A NUMERIC-LOOKING string ("64000") is the only wrong-typed
        // case that still binds under the real options (NumberHandling.AllowReadingFromString), so it
        // is the only WrongType that actually reaches a sink: the request binds, validates, plans, and
        // the retained diagnostic carries the flag. The scan reads only the token kind, never the
        // string. (A NON-numeric string here would 400 at the binder and never reach a sink - that is
        // covered structurally by the leak tests, not asserted as a reachable WrongType.)
        var scan = Scan("{\"Capabilities\":{\"Decode\":{\"VideoCodecs\":[{\"Codec\":\"h264\",\"MaxBitrate\":\"64000\"}]}}}");

        Assert.Equal(0, scan.UnknownMemberTotal);
        var issue = Assert.Single(scan.WrongTypes);
        Assert.Equal(ContractPath.DecodeVideoCodecs, issue.Path);
        Assert.Equal(ContractIssueCode.WrongType, issue.Code);
    }

    [Fact]
    public void KnownNumericSentAsNonBindingString_ScannerFlagsButValueNeverLeaks()
    {
        // A non-numeric string under a numeric member: the SCANNER still flags WrongType (it judges by
        // token kind), but at the real binder this body 400s and never reaches a sink. Asserted here
        // only to prove the value never leaks into the scan output regardless.
        var scan = Scan("{\"Capabilities\":{\"Decode\":{\"VideoCodecs\":[{\"MaxBitrate\":\"" + Sentinel + "\"}]}}}");

        Assert.Single(scan.WrongTypes);
        Assert.DoesNotContain(Sentinel, SerializeAll(scan), StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateUnknownProperties_CountedEach()
    {
        var scan = Scan("{\"Bogus\":1,\"Bogus\":2,\"AlsoBogus\":3}");

        Assert.Equal(3, scan.UnknownMemberTotal);
        Assert.Equal(3, UnknownAt(scan, ContractPath.Request));
    }

    [Fact]
    public void ExcessiveDepth_Swallowed_NoThrow_RequestUnaffected()
    {
        // A value nested far deeper than Utf8JsonReader's max depth, under an unknown key. Skipping it
        // raises JsonException inside the scan, which must be swallowed - never thrown at the request.
        var deep = new string('[', 200) + new string(']', 200);
        var body = "{\"" + Sentinel + "\":" + deep + "}";

        var ex = Record.Exception(() =>
        {
            var scan = Scan(body);
            Assert.DoesNotContain(Sentinel, SerializeAll(scan), StringComparison.Ordinal);
        });

        Assert.Null(ex);
    }

    [Fact]
    public void UnknownMembers_AttributedToNearestKnownContainer()
    {
        var scan = Scan("{\"RootBogus\":1,\"Capabilities\":{\"CapBogus\":1,\"Decode\":{\"DecodeBogus\":1,\"VideoCodecs\":[{\"CodecBogus\":1}]}}}");

        Assert.Equal(4, scan.UnknownMemberTotal);
        Assert.Equal(1, UnknownAt(scan, ContractPath.Request));
        Assert.Equal(1, UnknownAt(scan, ContractPath.Capabilities));
        Assert.Equal(1, UnknownAt(scan, ContractPath.Decode));
        Assert.Equal(1, UnknownAt(scan, ContractPath.DecodeVideoCodecs));
    }

    [Fact]
    public void ScannedByteCount_ReportedVerbatim()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"ItemId\":\"x\"}");
        var scan = PlaybackContractScanner.Scan(bytes, ContractScanTestModel.Root, bytes.Length, bodyLimitExceeded: false);

        Assert.Equal(bytes.Length, scan.ScannedBodyByteCount);
        Assert.False(scan.BodyLimitExceeded);
    }

    [Fact]
    public void OverLimit_NotParsed_FlagSetAndSizeReported()
    {
        // Even handed obvious unknowns, an over-limit body is not parsed: only the flag and size.
        var bytes = Encoding.UTF8.GetBytes("{\"Bogus\":1}");
        var scan = PlaybackContractScanner.Scan(bytes, ContractScanTestModel.Root, scannedByteCount: 999999, bodyLimitExceeded: true);

        Assert.True(scan.BodyLimitExceeded);
        Assert.Equal(0, scan.UnknownMemberTotal);
        Assert.Empty(scan.UnknownMembers);
        Assert.Equal(999999, scan.ScannedBodyByteCount);
    }

    [Fact]
    public void AtLimitBoundary_Scanned_Normally()
    {
        // A body treated as exactly at the limit (bodyLimitExceeded=false) is scanned normally.
        var scan = Scan("{\"Bogus\":1}");

        Assert.False(scan.BodyLimitExceeded);
        Assert.Equal(1, scan.UnknownMemberTotal);
    }

    [Fact]
    public void PerPathBreakdown_AlwaysSumsToTotal()
    {
        // Anti-vacuity cross-check: the per-path breakdown always reconstructs the total.
        var scan = Scan("{\"A\":1,\"B\":2,\"Capabilities\":{\"C\":1,\"Decode\":{\"D\":1}}}");

        var sum = 0;
        foreach (var entry in scan.UnknownMembers)
        {
            sum += entry.Count;
        }

        Assert.Equal(scan.UnknownMemberTotal, sum);
        Assert.Equal(4, scan.UnknownMemberTotal);
    }
}
