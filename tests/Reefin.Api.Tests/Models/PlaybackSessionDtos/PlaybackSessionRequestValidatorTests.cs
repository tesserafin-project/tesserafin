using System;
using Reefin.Api.Models.PlaybackSessionDtos;
using Reefin.Playback.Decision;
using Xunit;

namespace Reefin.Api.Tests.Models.PlaybackSessionDtos;

/// <summary>
/// Unit tests for <see cref="PlaybackSessionRequestValidator"/>: verifies the request is accepted
/// when internally consistent, and rejected (via <see cref="ArgumentException"/>, mapped to 400 by
/// <c>Reefin.Api.Middleware.ExceptionMiddleware</c>) for every declared-but-unusable inconsistency
/// the <see cref="Reefin.Playback.Decision"/> vocabulary can actually express.
/// </summary>
public static class PlaybackSessionRequestValidatorTests
{
    private static readonly Guid ItemId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private static ClientCapabilities ValidCapabilities() => new(
        Decode: new DecodeCapabilities(
            DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264"], ["aac"])],
            VideoCodecs: [],
            AudioCodecs: [],
            SubtitleDelivery: [],
            SupportsHls: false,
            SupportsDash: false),
        OutputProfiles: []);

    private static PlaybackConstraints ValidConstraints() => new(
        AllowDirectPlay: true,
        AllowDirectStream: true,
        AllowTranscoding: true,
        AllowVideoStreamCopy: true,
        AllowAudioStreamCopy: true,
        MaxBitrate: null,
        MaxAudioChannels: null,
        PreferredAudioStreamIndex: null,
        PreferredSubtitleStreamIndex: null,
        SubtitleMode: SubtitlePlaybackMode.Default,
        PreferredSubtitleLanguages: [],
        AlwaysBurnInSubtitleWhenTranscoding: false,
        StartTimeTicks: 0);

    private static CreatePlaybackSessionRequest ValidRequest() => new(ItemId, UserId, ValidCapabilities(), ValidConstraints());

    [Fact]
    public static void Validate_ConsistentRequest_DoesNotThrow()
    {
        var exception = Record.Exception(() => PlaybackSessionRequestValidator.Validate(ValidRequest()));

        Assert.Null(exception);
    }

    [Fact]
    public static void Validate_NoDecodeCapabilityDeclaredAtAll_Throws()
    {
        var capabilities = new ClientCapabilities(
            Decode: new DecodeCapabilities([], [], [], [], SupportsHls: false, SupportsDash: false),
            OutputProfiles: []);
        var request = ValidRequest() with { Capabilities = capabilities };

        var exception = Assert.Throws<ArgumentException>(() => PlaybackSessionRequestValidator.Validate(request));
        Assert.Contains("decode", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public static void Validate_NonPositiveVideoCodecMaxBitrate_Throws()
    {
        var capabilities = new ClientCapabilities(
            Decode: new DecodeCapabilities(
                [],
                [new VideoCodecCapability("h264", [], null, null, [], null, MaxBitrate: 0)],
                [],
                [],
                SupportsHls: false,
                SupportsDash: false),
            OutputProfiles: []);
        var request = ValidRequest() with { Capabilities = capabilities };

        var exception = Assert.Throws<ArgumentException>(() => PlaybackSessionRequestValidator.Validate(request));
        Assert.Contains("maxBitrate", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public static void Validate_DuplicateVideoCodecDeclaration_Throws()
    {
        var capabilities = new ClientCapabilities(
            Decode: new DecodeCapabilities(
                [],
                [
                    new VideoCodecCapability("h264", [], null, null, [], null, null),
                    new VideoCodecCapability("h264", ["high"], null, null, [], null, null),
                ],
                [],
                [],
                SupportsHls: false,
                SupportsDash: false),
            OutputProfiles: []);
        var request = ValidRequest() with { Capabilities = capabilities };

        var exception = Assert.Throws<ArgumentException>(() => PlaybackSessionRequestValidator.Validate(request));
        Assert.Contains("h264", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public static void Validate_NonPositiveConstraintsMaxBitrate_Throws()
    {
        var request = ValidRequest() with { Constraints = ValidConstraints() with { MaxBitrate = -1 } };

        Assert.Throws<ArgumentException>(() => PlaybackSessionRequestValidator.Validate(request));
    }

    [Fact]
    public static void Validate_NegativeStartTimeTicks_Throws()
    {
        var request = ValidRequest() with { Constraints = ValidConstraints() with { StartTimeTicks = -1 } };

        Assert.Throws<ArgumentException>(() => PlaybackSessionRequestValidator.Validate(request));
    }

    [Fact]
    public static void Validate_AllPlaybackMethodsForbidden_Throws()
    {
        var constraints = ValidConstraints() with { AllowDirectPlay = false, AllowDirectStream = false, AllowTranscoding = false };
        var request = ValidRequest() with { Constraints = constraints };

        var exception = Assert.Throws<ArgumentException>(() => PlaybackSessionRequestValidator.Validate(request));
        Assert.Contains("playback method", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public static void Validate_NullCapabilities_ThrowsWithoutCrashing()
    {
        var request = ValidRequest() with { Capabilities = null! };

        Assert.Throws<ArgumentException>(() => PlaybackSessionRequestValidator.Validate(request));
    }

    [Fact]
    public static void Validate_NullConstraints_ThrowsWithoutCrashing()
    {
        var request = ValidRequest() with { Constraints = null! };

        Assert.Throws<ArgumentException>(() => PlaybackSessionRequestValidator.Validate(request));
    }
}
