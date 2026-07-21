using Tesserafin.Playback.Decision;

namespace Tesserafin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// <see cref="OutputSpec"/> minus <see cref="OutputSpec.SubtitleFormat"/>, which has no counterpart
/// in the fixture schema's <c>expected.output</c> object.
/// </summary>
/// <param name="Container">See <see cref="OutputSpec.Container"/>.</param>
/// <param name="VideoCodec">See <see cref="OutputSpec.VideoCodec"/>.</param>
/// <param name="AudioCodec">See <see cref="OutputSpec.AudioCodec"/>.</param>
/// <param name="Resolution">See <see cref="OutputSpec.Resolution"/>.</param>
/// <param name="VideoRange">See <see cref="OutputSpec.VideoRange"/>.</param>
/// <param name="AudioChannels">See <see cref="OutputSpec.AudioChannels"/>.</param>
/// <param name="TotalBitrate">See <see cref="OutputSpec.TotalBitrate"/>.</param>
/// <param name="VideoBitrate">See <see cref="OutputSpec.VideoBitrate"/>.</param>
/// <param name="AudioBitrate">See <see cref="OutputSpec.AudioBitrate"/>.</param>
/// <param name="Protocol">See <see cref="OutputSpec.Protocol"/>.</param>
public sealed record PlaybackCompatFixtureOutput(
    string? Container,
    string? VideoCodec,
    string? AudioCodec,
    Resolution? Resolution,
    string? VideoRange,
    int? AudioChannels,
    int? TotalBitrate,
    int? VideoBitrate,
    int? AudioBitrate,
    StreamingProtocol Protocol);
