using System;
using Tesserafin.Api.Auth.PlaybackCapabilityPolicy;
using Tesserafin.Api.Helpers;
using Xunit;

namespace Tesserafin.Api.Tests.Helpers;

/// <summary>
/// #153-LTV-S1. Every uri form the Live TV HLS path was measured to emit, plus every form the
/// contract classifies without emitting.
/// </summary>
/// <remarks>
/// No capability value in this file is a real one, and none is a default on any type: they are
/// local literals that exist only to be compared against themselves.
/// </remarks>
public class HlsManifestCredentialPropagatorTests
{
    private const string Capability = "capability-under-test";
    private const string MediaSource = "6d5da76e3955fd1005f75c496c371521";

    private static readonly Uri _origin = new("http://127.0.0.1:8096");

    [Fact]
    public void CapabilityKey_IsTheKeyAuthenticationReads()
    {
        // Two constants that must never drift: the propagator writes what the handler reads.
        Assert.Equal(PlaybackCapabilityAuthenticationHandler.QueryKey, HlsManifestCredentialPropagator.CapabilityKey);
    }

    [Fact]
    public void RelativeSegmentWithNoQuery_GetsTheCapabilityAndTheMediaSource()
    {
        const string Manifest = "#EXTM3U\n#EXTINF:3.000000,\nhls/abc/abc0.ts\n";

        var result = HlsManifestCredentialPropagator.Propagate(Manifest, Capability, MediaSource, _origin);

        Assert.Equal(
            "#EXTM3U\n#EXTINF:3.000000,\nhls/abc/abc0.ts?playbackCapability=capability-under-test&mediaSourceId=" + MediaSource + "\n",
            result);
    }

    [Fact]
    public void RelativeSegment_WithNoMediaSourceBinding_GetsOnlyTheCapability()
    {
        var result = HlsManifestCredentialPropagator.Propagate("hls/abc/abc0.ts\n", Capability, null, _origin);

        Assert.Equal("hls/abc/abc0.ts?playbackCapability=capability-under-test\n", result);
    }

    [Fact]
    public void SegmentWithAnExistingQuery_KeepsItByteForByteAndAppendsOnce()
    {
        // `%2F` stays `%2F`. Parsing the query into pairs and writing it back would re-encode every
        // value, and "encoded exactly once" is the clause that would break.
        const string Manifest = "hls/abc/abc0.ts?tag=a%2Fb&n=1\n";

        var result = HlsManifestCredentialPropagator.Propagate(Manifest, Capability, null, _origin);

        Assert.Equal("hls/abc/abc0.ts?tag=a%2Fb&n=1&playbackCapability=capability-under-test\n", result);
    }

    [Fact]
    public void ValueNeedingEscaping_IsEncodedExactlyOnce()
    {
        var result = HlsManifestCredentialPropagator.Propagate("s0.ts\n", "a+b/c=d", null, _origin);

        Assert.Equal("s0.ts?playbackCapability=a%2Bb%2Fc%3Dd\n", result);
        Assert.DoesNotContain("%25", result, StringComparison.Ordinal);
    }

    [Fact]
    public void UriWithAFragment_KeepsTheFragmentLast()
    {
        var result = HlsManifestCredentialPropagator.Propagate("s0.ts?n=1#part\n", Capability, null, _origin);

        Assert.Equal("s0.ts?n=1&playbackCapability=capability-under-test#part\n", result);
    }

    [Fact]
    public void ExtXMap_IsRewrittenInsideItsQuotes()
    {
        const string Manifest = "#EXT-X-MAP:URI=\"hls/abc/abc-1.mp4\"\n#EXTINF:3.000000,\nhls/abc/abc0.mp4\n";

        var result = HlsManifestCredentialPropagator.Propagate(Manifest, Capability, MediaSource, _origin);

        Assert.Equal(
            "#EXT-X-MAP:URI=\"hls/abc/abc-1.mp4?playbackCapability=capability-under-test&mediaSourceId=" + MediaSource + "\"\n"
            + "#EXTINF:3.000000,\n"
            + "hls/abc/abc0.mp4?playbackCapability=capability-under-test&mediaSourceId=" + MediaSource + "\n",
            result);
    }

    [Theory]
    [InlineData("#EXT-X-KEY:METHOD=AES-128,URI=\"https://keys.example/k\"")]
    [InlineData("#EXT-X-SESSION-KEY:METHOD=AES-128,URI=\"https://keys.example/k\"")]
    [InlineData("#EXT-X-PART:DURATION=0.5,URI=\"p0.mp4\"")]
    [InlineData("#EXT-X-PRELOAD-HINT:TYPE=PART,URI=\"p1.mp4\"")]
    [InlineData("#EXT-X-RENDITION-REPORT:URI=\"../other/live.m3u8\"")]
    [InlineData("#EXT-X-SOMETHING-NEW:URI=\"x.ts\"")]
    public void AnyOtherTagCarryingAUri_IsRefused(string line)
    {
        // Fail closed. A manifest shape this transformer does not fully understand must not be
        // served with a credential in it, and a silently passed-through uri would reach the client
        // uncredentialed and look like a playback bug instead of an unhandled form.
        var exception = Assert.Throws<InvalidOperationException>(
            () => HlsManifestCredentialPropagator.Propagate("#EXTM3U\n" + line + "\n", Capability, null, _origin));

        Assert.Contains("does not classify", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CrlfManifest_StaysCrlf()
    {
        var result = HlsManifestCredentialPropagator.Propagate("#EXTM3U\r\ns0.ts\r\n", Capability, null, _origin);

        Assert.Equal("#EXTM3U\r\ns0.ts?playbackCapability=capability-under-test\r\n", result);
    }

    [Fact]
    public void MixedLineEndings_AreEachPreserved()
    {
        var result = HlsManifestCredentialPropagator.Propagate("#EXTM3U\r\ns0.ts\ns1.ts\r\n", Capability, null, _origin);

        Assert.Equal(
            "#EXTM3U\r\ns0.ts?playbackCapability=capability-under-test\ns1.ts?playbackCapability=capability-under-test\r\n",
            result);
    }

    [Fact]
    public void ManifestWithNoTrailingNewline_KeepsNotHavingOne()
    {
        var result = HlsManifestCredentialPropagator.Propagate("#EXTM3U\ns0.ts", Capability, null, _origin);

        Assert.Equal("#EXTM3U\ns0.ts?playbackCapability=capability-under-test", result);
    }

    [Fact]
    public void CommentsTagsAndBlankLines_AreUnchanged()
    {
        const string Manifest = "#EXTM3U\n#EXT-X-VERSION:3\n\n# a plain comment\n#EXT-X-TARGETDURATION:3\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:EVENT\n";

        var result = HlsManifestCredentialPropagator.Propagate(Manifest, Capability, MediaSource, _origin);

        Assert.Equal(Manifest, result);
    }

    [Fact]
    public void CapabilityAlreadyPresentAndIdentical_LeavesExactlyOne()
    {
        const string Manifest = "s0.ts?playbackCapability=capability-under-test\n";

        var result = HlsManifestCredentialPropagator.Propagate(Manifest, Capability, null, _origin);

        Assert.Equal(Manifest, result);
        Assert.Equal(1, CountOf(result, "playbackCapability="));
    }

    [Fact]
    public void CapabilityAlreadyPresentTwice_IsRefused()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => HlsManifestCredentialPropagator.Propagate(
                "s0.ts?playbackCapability=capability-under-test&playbackCapability=capability-under-test\n",
                Capability,
                null,
                _origin));

        Assert.Contains("more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADifferentCapabilityAlreadyPresent_IsRefused()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => HlsManifestCredentialPropagator.Propagate(
                "s0.ts?playbackCapability=some-other-value\n",
                Capability,
                null,
                _origin));

        Assert.Contains("a different", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaSourceAlreadyPresentAndIdentical_IsNotDuplicated()
    {
        var result = HlsManifestCredentialPropagator.Propagate(
            "s0.ts?mediaSourceId=" + MediaSource + "\n",
            Capability,
            MediaSource,
            _origin);

        Assert.Equal(1, CountOf(result, "mediaSourceId="));
    }

    [Fact]
    public void AbsoluteExternalUri_IsNeverEnriched()
    {
        const string Manifest = "#EXTM3U\nhttps://cdn.example.test/seg0.ts\n";

        var result = HlsManifestCredentialPropagator.Propagate(Manifest, Capability, MediaSource, _origin);

        Assert.Equal(Manifest, result);
    }

    [Fact]
    public void ProtocolRelativeExternalUri_IsNeverEnriched()
    {
        const string Manifest = "//cdn.example.test/seg0.ts\n";

        var result = HlsManifestCredentialPropagator.Propagate(Manifest, Capability, MediaSource, _origin);

        Assert.Equal(Manifest, result);
    }

    [Fact]
    public void ExternalUriInsideAnExtXMap_IsNeverEnriched()
    {
        const string Manifest = "#EXT-X-MAP:URI=\"https://cdn.example.test/init.mp4\"\n";

        var result = HlsManifestCredentialPropagator.Propagate(Manifest, Capability, MediaSource, _origin);

        Assert.Equal(Manifest, result);
    }

    [Fact]
    public void AbsoluteSameOriginUri_IsEnriched()
    {
        var result = HlsManifestCredentialPropagator.Propagate(
            "http://127.0.0.1:8096/videos/x/hls/abc/abc0.ts\n",
            Capability,
            null,
            _origin);

        Assert.Equal(
            "http://127.0.0.1:8096/videos/x/hls/abc/abc0.ts?playbackCapability=capability-under-test\n",
            result);
    }

    [Fact]
    public void RootRelativeUri_IsEnriched()
    {
        var result = HlsManifestCredentialPropagator.Propagate("/videos/x/hls/abc/abc0.ts\n", Capability, null, _origin);

        Assert.Equal("/videos/x/hls/abc/abc0.ts?playbackCapability=capability-under-test\n", result);
    }

    [Fact]
    public void ADurableTokenAlreadyInAUri_IsNeitherCopiedNorAdded()
    {
        // The transformer has no reader for a durable token and no writer for one. A uri that
        // arrives carrying `api_key` keeps its own bytes; nothing propagates it to the next uri.
        const string Manifest = "s0.ts?api_key=whatever\ns1.ts\n";

        var result = HlsManifestCredentialPropagator.Propagate(Manifest, Capability, null, _origin);

        Assert.Equal(
            "s0.ts?api_key=whatever&playbackCapability=capability-under-test\ns1.ts?playbackCapability=capability-under-test\n",
            result);
        Assert.Equal(1, CountOf(result, "api_key="));
    }

    [Theory]
    [InlineData("#EXTM3U\n#EXTINF:3.0,\nhls/abc/abc0.ts\n")]
    [InlineData("#EXT-X-MAP:URI=\"hls/abc/abc-1.mp4\"\nhls/abc/abc0.mp4\n")]
    public void NoDurableCredentialNameEverAppears(string manifest)
    {
        var result = HlsManifestCredentialPropagator.Propagate(manifest, Capability, MediaSource, _origin);

        foreach (var forbidden in new[] { "ApiKey", "api_key", "Authorization", "webSocketTicket" })
        {
            Assert.DoesNotContain(forbidden, result, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void EmptyCapability_IsRejectedRatherThanWrittenAsAnEmptyParameter()
    {
        Assert.Throws<ArgumentException>(
            () => HlsManifestCredentialPropagator.Propagate("s0.ts\n", string.Empty, null, _origin));
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
