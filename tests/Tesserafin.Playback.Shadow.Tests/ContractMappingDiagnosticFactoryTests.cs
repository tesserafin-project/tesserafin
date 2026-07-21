using System;
using System.Collections.Generic;
using Tesserafin.Playback.Contract.Diagnostics;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Dlna;
using Tesserafin.Playback.Shadow;
using Xunit;

namespace Tesserafin.Playback.Shadow.Tests;

/// <summary>
/// Issue #75's main signal, proven against a REAL round trip rather than a stub: a declared
/// <see cref="ClientCapabilities"/> is pushed through the production
/// <see cref="ReverseDlnaAdapter.ToDeviceProfile"/> (what
/// <c>PlaybackSessionsController</c> does to hand the v2 request body to the legacy
/// <c>StreamBuilder</c>) and back through <see cref="DlnaPlaybackAdapter.ToCapabilities"/> (what
/// <c>ShadowPlaybackSessionPlanner</c> does to feed the v2 engine). Whatever that pair actually
/// loses is what the diagnostic reports.
/// </summary>
/// <remarks>
/// No adapter behaviour is asserted as desirable here; these tests assert that a loss which really
/// happens is reported, named by <see cref="ContractPath"/>, and reported WITHOUT the lost value.
/// </remarks>
public sealed class ContractMappingDiagnosticFactoryTests
{
    private static ClientCapabilities RoundTrip(ClientCapabilities declared) =>
        DlnaPlaybackAdapter.ToCapabilities(ReverseDlnaAdapter.ToDeviceProfile(declared));

    private static DecodeCapabilities Decode(
        IReadOnlyList<DecodeProfile>? directPlay = null,
        IReadOnlyList<VideoCodecCapability>? video = null,
        IReadOnlyList<AudioCodecCapability>? audio = null,
        IReadOnlyList<SubtitleCapability>? subtitles = null,
        bool hls = false,
        bool dash = false) =>
        new(
            directPlay ?? [],
            video ?? [],
            audio ?? [],
            subtitles ?? [],
            hls,
            dash);

    /// <summary>
    /// Case (b), collection form: the client declares two per-codec limit entries for the SAME
    /// codec and the reverse mapper merges them into one <c>CodecProfile</c>, so only one entry
    /// comes back. <c>CountBefore &gt; CountAfter</c>, and the delta names only the affected path.
    /// </summary>
    [Fact]
    public void RoundTrip_ThatMergesDuplicateCodecEntries_ReportsCountLoss()
    {
        var declared = new ClientCapabilities(
            Decode(
                video:
                [
                    new VideoCodecCapability("h264", ["high"], 41.0, 8, ["SDR"], new Resolution(1920, 1080), 8_000_000),
                    new VideoCodecCapability("h264", ["main"], 30.0, 10, ["HDR10"], new Resolution(1280, 720), 4_000_000),
                ],
                audio:
                [
                    new AudioCodecCapability("aac", 6, 48_000, 16, 640_000),
                    new AudioCodecCapability("aac", 2, 44_100, 16, 320_000),
                ]),
            []);

        var diagnostic = ContractMappingDiagnosticFactory.Create(declared, RoundTrip(declared), 4096);

        Assert.NotNull(diagnostic);

        var video = Single(diagnostic!, ContractPath.DecodeVideoCodecs);
        Assert.True(video.CountBefore > video.CountAfter);
        Assert.Equal(2, video.CountBefore);
        Assert.Equal(1, video.CountAfter);

        var audio = Single(diagnostic, ContractPath.DecodeAudioCodecs);
        Assert.True(audio.CountBefore > audio.CountAfter);
        Assert.Equal(2, audio.CountBefore);
        Assert.Equal(1, audio.CountAfter);
    }

    /// <summary>
    /// Case (b), presence form: the client declares it can play HLS and DASH renditions. The legacy
    /// <c>DeviceProfile</c> the request has to be translated into has no dedicated slot for either
    /// flag - both are re-derived from its <c>TranscodingProfiles</c> - so a client that declares
    /// the capability without also declaring a matching transcoding target loses the declaration
    /// outright. <c>PresentBefore &amp;&amp; !PresentAfter</c>.
    /// </summary>
    [Fact]
    public void RoundTrip_ThatDropsDeclaredProtocolSupport_ReportsPresenceLoss()
    {
        var declared = new ClientCapabilities(Decode(hls: true, dash: true), []);

        var diagnostic = ContractMappingDiagnosticFactory.Create(declared, RoundTrip(declared), 512);

        Assert.NotNull(diagnostic);

        foreach (var path in new[] { ContractPath.DecodeSupportsHls, ContractPath.DecodeSupportsDash })
        {
            var delta = Single(diagnostic!, path);
            Assert.True(delta.PresentBefore);
            Assert.False(delta.PresentAfter);

            // A scalar member's counts stay 0 on both sides: a fabricated 1/0 would read as a
            // collection that lost an entry.
            Assert.Equal(0, delta.CountBefore);
            Assert.Equal(0, delta.CountAfter);
        }
    }

    /// <summary>
    /// The diagnostic names ONLY the affected paths. A member the round trip preserved - or grew,
    /// which the reverse mapper legitimately does by synthesising per-codec entries from declared
    /// direct-play combinations - produces no delta at all.
    /// </summary>
    [Fact]
    public void RoundTrip_ReportsOnlyTheAffectedPaths()
    {
        var declared = new ClientCapabilities(
            Decode(
                directPlay:
                [
                    new DecodeProfile(MediaKind.Video, ["mp4"], ["h264"], ["aac"]),
                    new DecodeProfile(MediaKind.Video, ["mkv"], ["hevc"], ["ac3"]),
                ],
                subtitles: [new SubtitleCapability("vtt", SubtitleDeliveryMethod.External)],
                hls: true),
            [
                new PlaybackOutputProfile(MediaKind.Video, StreamingProtocol.Hls, "ts", ["h264"], ["aac"], 5_000_000, 192_000, 2),
            ]);

        var mapped = RoundTrip(declared);
        var diagnostic = ContractMappingDiagnosticFactory.Create(declared, mapped, 8192);

        Assert.NotNull(diagnostic);

        // Everything declared here survives, and the per-codec lists actually GROW (synthesised from
        // the direct-play combinations) - a growth is not a loss and must not be reported.
        Assert.True(mapped.Decode.VideoCodecs.Count >= declared.Decode.VideoCodecs.Count);
        Assert.Empty(diagnostic!.Deltas);
    }

    /// <summary>
    /// The lost VALUE is never exposed - only how much was lost, and where. Nothing in the closure
    /// can hold one, and this pins that at the level of the produced instance too.
    /// </summary>
    [Fact]
    public void Diagnostic_ExposesCountsAndPathsOnly()
    {
        var declared = new ClientCapabilities(
            Decode(
                video:
                [
                    new VideoCodecCapability("h264", ["high"], 41.0, 8, ["SDR"], new Resolution(1920, 1080), 8_000_000),
                    new VideoCodecCapability("h264", ["main"], 30.0, 10, ["HDR10"], new Resolution(1280, 720), 4_000_000),
                ],
                hls: true),
            []);

        var diagnostic = ContractMappingDiagnosticFactory.Create(declared, RoundTrip(declared), 1234);

        Assert.NotNull(diagnostic);
        Assert.NotEmpty(diagnostic!.Deltas);
        Assert.All(
            diagnostic.Deltas,
            delta => Assert.NotEqual(ContractMember.None, delta.Path.Root));
    }

    /// <summary>
    /// <c>UnknownMemberTotal</c> is null and never 0. Option 1 does not scan the raw body, so the
    /// server does not KNOW how many unknown members arrived; 0 would be a lying zero,
    /// indistinguishable from "there were none".
    /// </summary>
    [Fact]
    public void Diagnostic_ReportsUnknownMembersAsUnknown_NeverAsZero()
    {
        var declared = new ClientCapabilities(Decode(hls: true), []);

        var diagnostic = ContractMappingDiagnosticFactory.Create(declared, RoundTrip(declared), null);

        Assert.NotNull(diagnostic);
        Assert.Null(diagnostic!.UnknownMemberTotal);
    }

    /// <summary>
    /// <c>PayloadSizeBytes</c> is null when Content-Length was absent (a chunked/streamed request),
    /// and carries the header value verbatim when present. Request buffering is never enabled to
    /// measure it, so "unknown" stays unknown.
    /// </summary>
    /// <param name="payloadSizeBytes">The Content-Length the request carried, or null for none.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(9001L)]
    public void Diagnostic_CarriesPayloadSizeVerbatim(long? payloadSizeBytes)
    {
        var declared = new ClientCapabilities(Decode(), []);

        var diagnostic = ContractMappingDiagnosticFactory.Create(declared, RoundTrip(declared), payloadSizeBytes);

        Assert.NotNull(diagnostic);
        Assert.Equal(payloadSizeBytes, diagnostic!.PayloadSizeBytes);
    }

    /// <summary>
    /// No <c>UnsupportedValue</c> is ever emitted for the free-form codec/container/profile members -
    /// deliberately, not by oversight. The only server-side signal that resembles a source for it
    /// (<c>IMediaEncoder.SupportsDecoder</c>/<c>SupportsEncoder</c>) answers whether THIS server can
    /// decode a codec, which says nothing about whether the codec is in the contract's vocabulary
    /// nor whether the CLIENT can read it - most obviously for a codec the server can DirectPlay
    /// without being able to decode it. A well-formed misspelling such as <c>av01</c> is therefore
    /// NOT detected, and this test pins that we do not pretend otherwise.
    /// </summary>
    [Fact]
    public void Diagnostic_NeverClaimsUnsupportedValue_ForAFreeFormCodecName()
    {
        var declared = new ClientCapabilities(
            Decode(
                directPlay: [new DecodeProfile(MediaKind.Video, ["mp4"], ["av01"], ["aac"])],
                video: [new VideoCodecCapability("av01", ["main"], null, null, ["SDR"], null, null)]),
            []);

        var diagnostic = ContractMappingDiagnosticFactory.Create(declared, RoundTrip(declared), 256);

        Assert.NotNull(diagnostic);
        Assert.Empty(diagnostic!.FieldIssues);
        Assert.DoesNotContain(diagnostic.FieldIssues, issue => issue.Code == ContractIssueCode.UnsupportedValue);
    }

    /// <summary>
    /// A caller with no declared domain capabilities (the legacy <c>MediaInfoHelper</c> path, which
    /// starts from a <c>DeviceProfile</c>) gets no diagnostic at all, rather than an empty one that
    /// would read as "nothing was lost".
    /// </summary>
    [Fact]
    public void Create_WithoutDeclaredCapabilities_ProducesNoDiagnostic()
    {
        var mapped = new ClientCapabilities(Decode(hls: true), []);

        Assert.Null(ContractMappingDiagnosticFactory.Create(null, mapped, 4096));
        Assert.Null(ContractMappingDiagnosticFactory.Create(mapped, null, 4096));
    }

    private static ContractMappingDelta Single(ContractMappingDiagnostic diagnostic, ContractPath path) =>
        Assert.Single(diagnostic.Deltas, delta => delta.Path == path);
}
